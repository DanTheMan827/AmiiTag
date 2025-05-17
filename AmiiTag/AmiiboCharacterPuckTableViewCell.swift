//
//  AmiiboCharacterTableViewCell.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/13/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import MobileCoreServices

class AmiiboCharacterPuckTableViewCell: UITableViewCell, LibraryPickerProtocol {
    var Info: PuckPeripheral.SlotInfo!
    var Puck: PuckPeripheral!
    var ViewController: AmiiboCharactersPuckTableViewController!
    fileprivate var dismiss = true
    @IBOutlet var CellImage: UIImageView!
    @IBOutlet var CellLabel: UILabel!
    
    @IBAction func clearTapped(_ sender: Any) {
        let alert = UIAlertController(title: "Please Wait", message: "Clearing Slot", preferredStyle: .alert)
        self.ViewController.present(alert, animated: true)
        
        Task {
            do {
                _ = try await Puck.clearSlot(slot: Info.slot)
                DispatchQueue.main.async {
                    self.ViewController.dismiss(animated: true) {
                        self.dismiss = false
                    }
                    
                    let tag = TagDump.getBlankTag()
                    self.ViewController.puckSlots[Int(self.Info.slot)].dump = tag
                    self.ViewController.puckSlots[Int(self.Info.slot)].name = tag.displayName
                    self.ViewController.puckSlots[Int(self.Info.slot)].idHex = "0x\(tag.fullHex)"
                    self.CellImage.image = tag.image
                    self.CellLabel.text = tag.displayName
                    if !tag.fullHex.hasSuffix("02") {
                        self.CellLabel.text = "Unknown Data"
                    }
                }
            } catch {
                DispatchQueue.main.async {
                    self.ViewController.dismiss(animated: true) {
                        self.ViewController.present(error.getAlertController(), animated: true)
                    }
                }
            }
        }
    }
    
    @IBAction func randomizeTapped(_ sender: Any) {
        let alert = UIAlertController(title: "Please Wait", message: "Reading \(Puck.name)", preferredStyle: .alert)
        self.ViewController.present(alert, animated: true)
        
        if (Puck.amiitoolEnabled && Puck.amiitoolKeysLoaded) {
            alert.message = "Randomizing Slot"
            Puck.randomizeUid(slot: Info.slot) { (result) in
                switch result {
                case .success(let tag):
                    DispatchQueue.main.async {
                        self.ViewController.dismiss(animated: true) {
                            self.dismiss = false
                        }
                    }
                    break
                    
                case .failure(let error):
                    DispatchQueue.main.async {
                        self.ViewController.dismiss(animated: true) {
                            self.ViewController.present(error.getAlertController(), animated: true)
                        }
                    }
                    break
                }
            }
        } else {
            Puck.readTag(slot: Info.slot) { (result) in
                DispatchQueue.main.async {
                    switch result {
                    case .status(let status):
                        alert.message = "Reading \(self.Puck.name) (\(status.start)/\(status.total))"
                    case .success(let tag):
                        self.ViewController.dismiss(animated: true) {
                            if let dump = try? TagDump(data: tag) {
                                if dump.isAmiibo {
                                    self.dismiss = false
                                    _ = self.AmiiboCharacterPicked(tag: try! dump.randomizeUID())
                                } else {
                                    self.ViewController.present(AmiiTagError(description: "Not an amiibo").getAlertController(), animated: true)
                                }
                            } else {
                                self.ViewController.present(AmiiTagError(description: "Error reading tag").getAlertController(), animated: true)
                            }
                        }
                        break
                    case .failure(let error):
                        self.ViewController.dismiss(animated: true) {
                            self.ViewController.present(error.getAlertController(), animated: true)
                        }
                        break
                    }
                }
            }
        }
    }
    
    @IBAction func downloadTapped(_ sender: Any) {
        let alert = UIAlertController(title: "Please Wait", message: "Reading \(Puck.name)", preferredStyle: .alert)
        self.ViewController.present(alert, animated: true)
        
        Puck.readTag(slot: Info.slot) { (result) in
            DispatchQueue.main.async {
                switch result {
                case .status(let status):
                    alert.message = "Reading \(self.Puck.name) (\(status.start)/\(status.total))"
                case .success(let tag):
                    self.ViewController.dismiss(animated: true) {
                        TagInfoViewController.openTagInfo(dump: TagDump(data: tag)!, controller: self.ViewController)
                    }
                    break
                case .failure(let error):
                    self.ViewController.dismiss(animated: true) {
                        self.ViewController.present(error.getAlertController(), animated: true)
                    }
                    break
                }
            }
        }
    }
    
    @IBAction func uploadTapped(_ sender: Any) {
        let alertController = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet).Popify(view: ViewController.view)
        
        alertController.addAction(UIAlertAction(title: "Amiibo Library", style: .default, handler: { (action) in
            LibraryPicker.ShowPicker(using: self.ViewController, with: self)
        }))
        
        alertController.addAction(UIAlertAction(title: "Load Tag", style: .default, handler: { (action) in
            AmiiboFilePicker.OpenAmiibo(PresentingViewController: self.ViewController) { (result) in
                switch result {
                case .success(let tag):
                    self.dismiss = false
                    _ = self.AmiiboCharacterPicked(tag: tag)
                case .failure(let error):
                    self.ViewController.present(error.getAlertController(), animated: true)
                    break
                }
            }
        }))
        
        alertController.addAction(UIAlertAction(title: "NFC", style: .default, handler: { (action) in
            NFCTagReader.ReadAmiibo { (result) in
                switch result {
                case .success(let tag):
                    self.dismiss = false
                    _ = self.AmiiboCharacterPicked(tag: tag)
                case .failure(_):
                    break
                }
            }
        }))
        
        alertController.addAction(UIAlertAction(title: "QR Code", style: .default, handler: { (action) in
            ScannerViewController.ShowScanner(PresentingViewController: self.ViewController) { (result) in
                switch result {
                case .success(let tag):
                    self.dismiss = false
                    _ = self.AmiiboCharacterPicked(tag: tag)
                case .failure(let error):
                    self.ViewController.present(error.getAlertController(), animated: true)
                    break
                }
            }
        }))
        
        alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel, handler: nil))
        
        self.ViewController.present(alertController, animated: true)
    }
    
    // LibraryPickerProtocol
    func AmiiboSeriesPicked(series: String) -> Bool {
        return true
    }
    
    func AmiiboCharacterPicked(tag: TagDump) -> Bool {
        if self.dismiss {
            self.ViewController.dismiss(animated: true) {
                self.dismiss = false
                let _ = self.AmiiboCharacterPicked(tag: tag)
            }
            
            return true
        }
        
        let alert = UIAlertController(title: "Please Wait", message: "Writing \(Puck.name)", preferredStyle: .alert)
        dismiss = true
        self.ViewController.present(alert, animated: true)
        
        Puck.writeTag(toSlot: Info.slot, using: tag.data) { (result) in
            DispatchQueue.main.async {
                switch result {
                case .status(let status):
                    alert.message = "Writing \(self.Puck.name) (\(status.start)/\(status.total))"
                case .success(()):
                    self.ViewController.puckSlots[Int(self.Info.slot)].dump = tag
                    self.ViewController.puckSlots[Int(self.Info.slot)].name = tag.displayName
                    self.ViewController.puckSlots[Int(self.Info.slot)].idHex = "0x\(tag.fullHex)"
                    self.CellImage.image = tag.image
                    self.CellLabel.text = tag.displayName
                    if !tag.fullHex.hasSuffix("02") {
                        self.CellLabel.text = "Unknown Data"
                    }
                    self.Puck.getSlotSummary { (result) in
                        DispatchQueue.main.async {
                            switch result {
                            case .success(let summary):
                                self.ViewController.dismiss(animated: true)
                                
                                break
                            case .failure(let error):
                                self.ViewController.dismiss(animated: true) {
                                    self.ViewController.present(error.getAlertController(), animated: true)
                                }
                                
                                break
                            }
                        }
                    }
                    break
                case .failure(let error):
                    self.ViewController.dismiss(animated: true) {
                        self.ViewController.present(error.getAlertController(), animated: true)
                    }
                    break
                }
            }
        }
        
        return false
    }
}
