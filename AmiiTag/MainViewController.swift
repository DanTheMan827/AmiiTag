//
//  MainView.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/28/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import CoreNFC
import CoreBluetooth
import MobileCoreServices
import SwiftyBluetooth

class MainViewController: UIViewController, LibraryPickerProtocol {
    static var main: MainViewController?
    @IBOutlet var logo: UIImageView!
    
    @IBAction func loadTagTap(_ sender: Any) {
        AmiiboFilePicker.OpenAmiibo(PresentingViewController: self) { (result) in
            switch result {
            case .success(let tag):
                TagInfoViewController.openTagInfo(dump: tag, controller: self)
                break
            case .failure(let error):
                self.present(error.getAlertController(), animated: true, completion: nil)
                break
            }
        }
    }
    
    @IBAction func startTagReadingSession() {
        if (PuckPeripheral.pucks.count > 0) {
            let alertController = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet)
            alertController.view.tintColor = self.view.tintColor
            
            for puck in PuckPeripheral.pucks.sorted(by: { (a, b) -> Bool in
                return a.name > b.name
            }) {
                alertController.addAction(UIAlertAction(title: puck.name, style: .default, handler: { (action) in
                    let alert = UIAlertController(title: "Please Wait", message: "Reading " + (puck.name), preferredStyle: .alert)
                    self.present(alert, animated: true)
                    
                    puck.readTag { (result) in
                        switch result {
                        case .status(let status):
                            alert.message = "Reading \(puck.name) (\(status.start)/\(status.total))"
                        case .success(let tag):
                            self.dismiss(animated: true)
                            TagInfoViewController.openTagInfo(dump: TagDump(data: tag)!, controller: self)
                            puck.disconnect { (result) in
                                PuckPeripheral.startScanning()
                            }
                        case .failure(let error):
                            self.dismiss(animated: true)
                            self.present(error.getAlertController(), animated: true)
                            puck.disconnect { (result) in
                                PuckPeripheral.startScanning()
                            }
                        }
                        
                        
                    }
                    
                }))
            }
            
            alertController.addAction(UIAlertAction(title: "NFC", style: .default){ action -> Void in
                self.startNfcTagReadingSession()
            })
            
            alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel){ action -> Void in })
            self.present(alertController, animated:true){}
        } else {
            self.startNfcTagReadingSession()
        }
    }
    
    func startNfcTagReadingSession() {
        NFCTagReader.ReadAmiibo { (result) in
            switch (result) {
            case .success(let tag):
                TagInfoViewController.openTagInfo(dump: tag, controller: self)
                break
            case .failure(let error):
                self.present(error.getAlertController(), animated: true, completion: nil)
            }
        }
    }
    
    @IBAction func scanQrTap(_ sender: Any) {
        ScannerViewController.ShowScanner(PresentingViewController: self) { (result) in
            switch result {
            case .success(let tag):
                TagInfoViewController.openTagInfo(dump: tag, controller: self)
                break
            case .failure(let error):
                self.present(error.getAlertController(), animated: true, completion: nil)
            }
        }
    }
    
    @IBAction func managePuckTap(_ sender: Any) {
        if let alertController = PuckPeripheral.getPuckChooser(puckChosen: { (puck) in
            let alert = UIAlertController(title: "Please Wait", message: "Reading " + (puck.name), preferredStyle: .alert)
            self.present(alert, animated: true)
            
            puck.getAllSlotInformation { (result) in
                switch result {
                case .status(let status):
                    alert.message = "Reading \(puck.name) (\(status.current + 1)/\(status.total))"
                case .success(let data):
                    self.dismiss(animated: true, completion: nil)
                    let storyboard = UIStoryboard(name: "Main", bundle: nil)
                    guard let view = storyboard.instantiateViewController(withIdentifier: "AmiiboCharactersPuck") as? AmiiboCharactersPuckTableViewController else {
                        puck.disconnect { (result) in }
                        return
                    }
                    
                    view.puck = puck
                    view.puckSlots = data
                    view.title = puck.name
                    let navigationController = UINavigationController(rootViewController: view)
                    navigationController.setToolbarHidden(false, animated: false)
                    self.present(navigationController, animated: true, completion: nil)
                    
                    break
                case .failure(let error):
                    self.dismiss(animated: true, completion: nil)
                    self.present(error.getAlertController(), animated: true, completion: nil)
                    puck.disconnect { (result) in }
                    break
                }
            }
        }) {
            present(alertController, animated: true, completion: nil)
        }
    }
    
    @IBAction func amiiboLibraryTap(_ sender: Any) {
        LibraryPicker.ShowPicker(using: self, with: self)
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        MainViewController.main = self
        
        print("Loaded \(NTAG215Tag.uidSignatures.count) UID/Signature pairs")
        print("Loaded \(AmiiboDatabase.amiiboDumps.count) amiibo dumps")
        
        NotificationCenter.default.addObserver(forName: Central.CentralStateChange, object: Central.sharedInstance, queue: nil) { (notification) in
            if let state = notification.userInfo?["state"] as? CBManagerState {
                if state == .poweredOff {
                    if AmiiboCharactersPuckTableViewController.showing {
                        self.dismiss(animated: true, completion: nil)
                    }
                }
            }
        }
    }
    
    func AmiiboSeriesPicked(series: String) -> Bool {
        return true
    }
    
    func AmiiboCharacterPicked(tag: TagDump) -> Bool {
        return true
    }
}
