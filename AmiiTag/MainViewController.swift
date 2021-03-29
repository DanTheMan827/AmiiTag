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
                self.present(error.getAlertController(), animated: true)
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
                            self.dismiss(animated: true) {
                                TagInfoViewController.openTagInfo(dump: TagDump(data: tag)!, controller: self)
                            }
                            puck.disconnect { (result) in
                                PuckPeripheral.startScanning()
                            }
                        case .failure(let error):
                            self.dismiss(animated: true) {
                                self.present(error.getAlertController(), animated: true)
                            }
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
                self.present(error.getAlertController(), animated: true)
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
                self.present(error.getAlertController(), animated: true)
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
                    self.dismiss(animated: true) {
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
                        self.present(navigationController, animated: true)
                    }
                    break
                case .failure(let error):
                    self.dismiss(animated: true) {
                        self.present(error.getAlertController(), animated: true)
                    }
                    puck.disconnect { (result) in }
                    break
                }
            }
        }) {
            present(alertController, animated: true)
        }
    }
    
    @IBAction func amiiboLibraryTap(_ sender: Any) {
        LibraryPicker.ShowPicker(using: self, with: self)
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        MainViewController.main = self
        let documentsPath = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let signaturesPath = documentsPath.appendingPathComponent("Signatures")
        let readmePath = documentsPath.appendingPathComponent("readme.txt")
        let signaturesReadmePath = signaturesPath.appendingPathComponent("readme.txt")
        
        let _  = try? FileManager.default.createDirectory(at: signaturesPath, withIntermediateDirectories: true)
        
        if !FileManager.default.fileExists(atPath: readmePath.path) {
            let _ = try? Data("Place key_retail.bin in this folder".bytes).write(to: readmePath)
        }
        
        if !FileManager.default.fileExists(atPath: signaturesReadmePath.path) {
            let _ = try? Data("This folder contains 42 byte binary files containing the first 10 bytes of scanned NTAG 215 tags followed by the 32 byte IC signature.".bytes).write(to: signaturesReadmePath)
        }
        
        AmiiboDatabase.LoadJson()
        
        print("Loaded \(NTAG215Tag.uidSignatures.count) UID/Signature pairs")
        
        NotificationCenter.default.addObserver(forName: Central.CentralStateChange, object: Central.sharedInstance, queue: nil) { (notification) in
            if let state = notification.userInfo?["state"] as? CBManagerState {
                if state == .poweredOff {
                    if AmiiboCharactersPuckTableViewController.showing {
                        self.dismiss(animated: true)
                    }
                }
            }
        }
        
        let tapGestureRecognizer = UITapGestureRecognizer(target: self, action: #selector(logoTapped(tapGestureRecognizer:)))
        logo.isUserInteractionEnabled = true
        logo.addGestureRecognizer(tapGestureRecognizer)
    }

    @objc func logoTapped(tapGestureRecognizer: UITapGestureRecognizer)
    {
        let alert = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet)
        
        alert.addAction(UIAlertAction(title: "Update Database", style: .default, handler: { (action) in
            self.updateDatabase()
        }))
        
        if !KeyFiles.hasKeys {
            alert.addAction(UIAlertAction(title: "Load key file", style: .default, handler: { (action) in
                KeyFiles.pickKeyFile(PresentingViewController: self) { (result) in
                    switch result {
                    case .success(()):
                        break
                    case .failure(let error):
                        self.present(error.getAlertController(), animated: true)
                    }
                }
            }))
        }
        
        alert.addAction(UIAlertAction(title: "Cancel", style: .default, handler: nil))
        
        self.present(alert, animated: true)
    }

    
    override func viewDidAppear(_ animated: Bool) {
        checkUpdate()
    }
    
    func updateDatabase() {
        let alert = UIAlertController(title: "Updating Database", message: "", preferredStyle: .alert)
        self.present(alert, animated: true)
        
        AmiiboDatabase.UpdateDatabase { (result) in
            DispatchQueue.main.async {
                switch result {
                case .success(_):
                    AmiiboDatabase.LoadJson()
                    self.dismiss(animated: true)
                    
                case .status(let status):
                    alert.message = "\(status.total - status.remaining) / \(status.total)\n\(status.url.remote.lastPathComponent)"
                    
                case .failure(let error):
                    self.dismiss(animated: true) {
                        self.present(error.getAlertController(), animated: true)
                    }
                }
            }
        }
    }
    
    func checkUpdate() {
        AmiiboDatabase.NeedsUpdate { (result) in
            DispatchQueue.main.async {
                switch result {
                case .success(let needsUpdate):
                    if needsUpdate {
                        let alert = UIAlertController(title: "Database Update", message: "There is a new database version available, would you like to update?", preferredStyle: .alert)
                        alert.addAction(UIAlertAction(title: "Yes", style: .default, handler: { (action) in
                            self.updateDatabase()
                        }))
                        alert.addAction(UIAlertAction(title: "No", style: .cancel))
                        
                        self.present(alert, animated: true)
                    }
                    
                case .failure(let error):
                    print(error)
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
