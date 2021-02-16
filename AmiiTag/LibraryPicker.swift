//
//  AmiiboPicker.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 2/14/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

protocol LibraryPickerProtocol {
    func AmiiboSeriesPicked(series: String) -> Bool
    func AmiiboCharacterPicked(tag: TagDump) -> Bool
}
class LibraryPicker {
    static func ShowPicker(using viewController: UIViewController, with delegate: LibraryPickerProtocol) {
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let view = storyboard.instantiateViewController(withIdentifier: "AmiiboSeries") as? AmiiboSeriesTableViewController else {
            return
        }
        view.title = "Amiibo Library"
        view.pickerDelegate = delegate
        viewController.present(UINavigationController(rootViewController: view), animated: true)
    }
}
